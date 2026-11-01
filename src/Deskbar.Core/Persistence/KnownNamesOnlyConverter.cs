using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deskbar.Core.Persistence;

// Reads a list of enum names and skips the ones this build does not know, where JsonStringEnumConverter would
// throw. A throw while parsing state.json makes DurableFile treat the whole file as corrupt: it moves it aside
// and falls back to the older .previous copy. A newer build that adds a StripItem and sees it hidden would then
// make an older build lose everything else in the file, which is the opposite of "keeps what it understands"
// (see AppState.WrittenBy and Extra).
//
// Only for lists whose members are independent, such as the hidden strip items: dropping one name changes
// nothing about the others. The known limit is the same as for nested fields in Extra: when the older build
// saves, the name it skipped is gone, so that item shows again in the newer build.
//
// Reads what JsonStringEnumConverter reads (names in any case, and the bare numbers of files written before
// enums were saved by name) and writes what it writes (names), so state.json does not change.
public sealed class KnownNamesOnlyConverter<T> : JsonConverter<IReadOnlyList<T>> where T : struct, Enum
{
    // A null list would otherwise reach the property as null, and callers expect the empty list.
    public override bool HandleNull => true;

    public override IReadOnlyList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return [];
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException($"Expected an array of {typeof(T).Name} names.");

        var known = new List<T>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String
                && Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var named) && Enum.IsDefined(named))
                known.Add(named);
            else if (reader.TokenType == JsonTokenType.Number
                && reader.TryGetInt32(out var number) && Enum.IsDefined(typeof(T), number))
                known.Add((T)Enum.ToObject(typeof(T), number));
            else
                reader.Skip();   // an unknown name, or something that is not a name at all; Skip passes over a nested value whole
        }
        return known;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<T> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value) writer.WriteStringValue(item.ToString());
        writer.WriteEndArray();
    }
}
