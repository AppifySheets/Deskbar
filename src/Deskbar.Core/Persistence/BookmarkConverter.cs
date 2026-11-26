using System.Text.Json;
using System.Text.Json.Serialization;
using Deskbar.Core.Bookmarks;

namespace Deskbar.Core.Persistence;

// Reads one Workspace.Bookmarks entry ON ITS OWN, so a bookmark that cannot be read costs that bookmark and
// nothing else. Same reasoning as ScreenTileLayoutConverter: a throw from inside the one Deserialize<AppState> call
// reads as a corrupt file to DurableFile.Read, which falls back to state.json.previous and loses every change since
// that copy, for the sake of one bookmark (an unknown Kind, a wrong token, a null Target).
//
// An entry that fails, or that parses but is no usable bookmark (Acceptable), comes back as null, and
// JsonPersistenceStore drops null entries in its post-load pass. Written exactly as the record would be written
// without this converter, through a private twin, so the file's shape is the plain record shape.
public sealed class BookmarkConverter : JsonConverter<Bookmark>
{
    // The entry's shape on disk. A type of its own, so (de)serializing it does not come back to this converter.
    sealed record Entry(Guid Id, string Name, BookmarkKind Kind, string Target, string? Arguments = null, string? WorkingDirectory = null);

    // Whether a parsed bookmark is usable: exactly what the manager would have accepted, so a hand-edited file cannot
    // hold what the menu refuses. A Name or Link target that only needs trimming is accepted as it stands.
    static bool Acceptable(Bookmark b) => BookmarkRules.Check(b).IsSuccess;

    public override Bookmark? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var entry = JsonDocument.ParseValue(ref reader);
        try
        {
            return entry.RootElement.Deserialize<Entry>(options) is { } e
                   && new Bookmark(e.Id, e.Name, e.Kind, e.Target, e.Arguments, e.WorkingDirectory) is var bookmark
                   && Acceptable(bookmark)
                ? bookmark
                : null;
        }
        // What System.Text.Json raises for a value of the wrong shape (an unknown enum name, a wrong token).
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, Bookmark value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new Entry(value.Id, value.Name, value.Kind, value.Target, value.Arguments, value.WorkingDirectory), options);
}
