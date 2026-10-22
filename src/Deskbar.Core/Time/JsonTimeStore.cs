using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Time;

// time.json, beside state.json. Written and read through DurableFile, as JsonPersistenceStore is, for
// the same reason: neither a crash mid-write nor a restart may destroy what was already tracked.
//
// Stored as a flat list of rows rather than as the nested dictionary the model uses. Guid and
// DateOnly keys serialise awkwardly and read badly, and this file is meant to be openable:
//
//     { "Rows": [ { "Workspace": "…guid…", "Day": "2026-08-09", "Seconds": 3600 } ] }
//
// Seconds rather than a TimeSpan, because System.Text.Json writes TimeSpan as "01:00:00" and the
// point of a number here is that a spreadsheet can add it up.
public sealed class JsonTimeStore(string baseDirectory) : ITimeStore
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public sealed record Row(Guid Workspace, DateOnly Day, double Seconds);

    sealed record File_(IReadOnlyList<Row> Rows);

    string TimePath => Path.Combine(baseDirectory, "time.json");

    // Missing file is the normal first-run case. An unreadable one falls back to the previous copy
    // (DurableFile.Read), because tracked history is not reconstructible from anywhere else; only
    // when both are unreadable is it a failure.
    public Result<WorkspaceTime> Load() =>
        DurableFile.Read(TimePath,
            text => Rebuild(JsonSerializer.Deserialize<File_>(text, Options) ?? throw new JsonException("time.json deserialized to null")),
            WorkspaceTime.Empty);

    static WorkspaceTime Rebuild(File_ file) =>
        file.Rows.Aggregate(WorkspaceTime.Empty, (time, row) =>
            time.Credit(row.Workspace, row.Day, TimeSpan.FromSeconds(row.Seconds)));

    public Result Save(WorkspaceTime time) =>
        Result.Try(() =>
        {
            Directory.CreateDirectory(baseDirectory);
            var rows = time.ByWorkspace
                .SelectMany(w => w.Value.Select(d => new Row(w.Key, d.Key, Math.Round(d.Value.TotalSeconds))))
                // Ordered so a diff of this file reads as "one day changed" rather than as a
                // reshuffle -- dictionary order is not stable across runs.
                .OrderBy(r => r.Workspace).ThenBy(r => r.Day)
                .ToList();

            // Flushed and swapped in with the old file kept as time.json.previous, as state.json is.
            DurableFile.Write(TimePath, JsonSerializer.Serialize(new File_(rows), Options));
        }, e => $"Could not write {TimePath}: {e.Message}");
}
