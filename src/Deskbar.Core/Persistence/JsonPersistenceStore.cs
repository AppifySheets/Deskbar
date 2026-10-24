using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Persistence;

// `trace` hears about a fallback to the previous copy (DurableFile), which is otherwise silent by design: the
// workspaces come back, and the line in the trace is how anyone learns the latest file was lost.
//
// `runningVersion` is this build's version. Save stamps it into state.json as WrittenBy and Load refuses a file
// stamped by a newer major version (StateVersionGate). Null (the tests, mostly) stamps nothing and refuses
// nothing. Top-level fields this build does not know ride through Load and Save in AppState.Extra.
public sealed class JsonPersistenceStore(string baseDirectory, Action<string>? trace = null, string? runningVersion = null) : IPersistenceStore
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
            AppState.Empty, trace)
        // The gate runs AFTER the read, never inside the parse: a throw there would look like a corrupt file and
        // make DurableFile move the newer file aside and fall back to .previous, which is the opposite of
        // protecting it.
        .Bind(state => runningVersion is null
            ? Result.Success(state)
            : StateVersionGate.Check(state.WrittenBy, runningVersion).Map(() => state));

    // Flushed to the disk, then swapped in with the old file kept as state.json.previous (DurableFile.Write), so
    // neither a crash mid-write nor the machine restarting can leave nothing readable behind.
    public Result Save(AppState state) =>
        Result.Try(() =>
        {
            Directory.CreateDirectory(baseDirectory);
            DurableFile.Write(StatePath, JsonSerializer.Serialize(runningVersion is null ? state : state with { WrittenBy = runningVersion }, Options));
        }, e => $"Could not write {StatePath}: {e.Message}");
}
