using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Persistence;

// `trace` hears about a fallback to the previous copy (DurableFile), which is otherwise silent by design: the
// workspaces come back, and the line in the trace is how anyone learns the latest file was lost.
public sealed class JsonPersistenceStore(string baseDirectory, Action<string>? trace = null) : IPersistenceStore
{
    // Finding 5 (reviewer, Important): without a string converter, RuleMatchKind (and any
    // future enum) serializes as a bare int -- unreadable in state.json and a silent
    // renumbering hazard if the enum's member order ever changes. JsonStringEnumConverter
    // still DESERIALIZES bare ints too (back-compat with any state.json written before this
    // fix), it just never WRITES them again.
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    string StatePath => Path.Combine(baseDirectory, "state.json");

    // Missing file is the normal first-run case -> Empty. An unreadable state.json falls back to the previous
    // copy (DurableFile.Read); only when that cannot be read either is it a real failure the caller must
    // surface (we refuse to silently overwrite user data).
    public Result<AppState> Load() =>
        DurableFile.Read(StatePath,
            text => JsonSerializer.Deserialize<AppState>(text, Options) ?? throw new JsonException("state.json deserialized to null"),
            AppState.Empty, trace);

    // Flushed to the disk, then swapped in with the old file kept as state.json.previous (DurableFile.Write), so
    // neither a crash mid-write nor the machine restarting can leave nothing readable behind.
    public Result Save(AppState state) =>
        Result.Try(() =>
        {
            Directory.CreateDirectory(baseDirectory);
            DurableFile.Write(StatePath, JsonSerializer.Serialize(state, Options));
        }, e => $"Could not write {StatePath}: {e.Message}");
}
