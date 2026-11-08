using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deskbar.Core.Domain;

// An immutable list that compares by CONTENTS. A record compares a list member by reference, so a record
// holding a plain list is unequal to an identical record built from another list instance (for example one
// loaded from state.json). Using this type for such a member keeps the record's generated equality correct
// without a hand-written Equals that every added property would have to remember.
//
// [CollectionBuilder] lets `[]` and `[a, b]` build one. On disk it is a plain JSON array, via the converter.
[CollectionBuilder(typeof(ValueList), nameof(ValueList.Create))]
[JsonConverter(typeof(ValueListConverterFactory))]
public sealed class ValueList<T>(IEnumerable<T> items) : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    readonly IReadOnlyList<T> items = items.ToList();

    public int Count => items.Count;
    public T this[int index] => items[index];
    public IEnumerator<T> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(ValueList<T>? other) => other is not null && items.SequenceEqual(other.items);
    public override bool Equals(object? obj) => obj is ValueList<T> other && Equals(other);
    public override int GetHashCode() => items.Aggregate(0, (hash, item) => HashCode.Combine(hash, item));
}

public static class ValueList
{
    // The collection builder for `[...]` expressions.
    public static ValueList<T> Create<T>(ReadOnlySpan<T> items) => new(items.ToArray());

    public static ValueList<T> ToValueList<T>(this IEnumerable<T> items) => new(items);
}

// Reads and writes a ValueList as a plain JSON array, so the file looks the same as with a List. A JSON `null`
// (a hand-edited state.json) reads as an EMPTY list, never as null: code reads these lists without a null check,
// and the Tiler's state key would throw on one (final review, item 8).
public sealed class ValueListConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ValueList<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(ValueListConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
}

public sealed class ValueListConverter<T> : JsonConverter<ValueList<T>>
{
    // Without this, System.Text.Json answers a JSON null with null itself and never calls Read. With it, Read sees
    // the null token too, and Write sees a null value.
    public override bool HandleNull => true;

    public override ValueList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        (JsonSerializer.Deserialize<List<T>>(ref reader, options) ?? []).ToValueList();

    // A null in memory is written as an empty array, the same thing it would load back as.
    public override void Write(Utf8JsonWriter writer, ValueList<T>? value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value?.ToList() ?? [], options);
}
