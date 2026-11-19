using System.Text.Json;
using System.Text.Json.Serialization;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Persistence;

// Reads one Workspace.TileLayouts entry ON ITS OWN, so a tree that cannot be read costs that entry and nothing else
// (whole-branch review, item 4).
//
// Why it is needed: TileNode is polymorphic on "kind" ("leaf" or "split", TileLayout.cs). A node with a missing or
// unknown kind makes System.Text.Json throw from inside the one Deserialize<AppState> call, and DurableFile.Read takes
// any throw in the parse for a corrupt file: it moves state.json aside and loads state.json.previous, so every change
// since that copy (workspaces, rules, renames) is lost for the sake of one screen's tiling layout. Here the entry is
// parsed into a JsonElement first (that only fails on JSON that is not JSON at all, which IS a corrupt file), then
// turned into an entry inside a try; an entry that fails comes back as null, and JsonPersistenceStore.MigrateTileSizes
// drops null entries along with the ones that parse but are no layout. That screen goes back to the automatic grid.
//
// Written exactly as the record would be written without this converter ({"Screen": ..., "Layout": ...}), through a
// private twin of the record, so the file's shape does not change and an older build reads it as before.
public sealed class ScreenTileLayoutConverter : JsonConverter<ScreenTileLayout>
{
    // The entry's shape on disk. A type of its own, so (de)serializing it does not come back to this converter.
    sealed record Entry(string Screen, TileLayout Layout);

    public override ScreenTileLayout? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var entry = JsonDocument.ParseValue(ref reader);
        try
        {
            return entry.RootElement.Deserialize<Entry>(options) is { } read ? new ScreenTileLayout(read.Screen, read.Layout) : null;
        }
        // What System.Text.Json raises for a value of the wrong shape: JsonException for a wrong token or an unknown
        // discriminator, NotSupportedException for an abstract TileNode with no discriminator at all.
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, ScreenTileLayout value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new Entry(value.Screen, value.Layout), options);
}
