// New: System.Text.Json support for the engine's shapes, so the data layer, the
// snapshots and the parity harness read and write them the same way.
//
// - Property names are camelCase, as the TypeScript objects' are (rows that keep
//   the database's snake_case say so with [JsonPropertyName]).
// - A JsMap with string keys is a JSON object, its keys in insertion order -
//   what JSON.stringify writes for a plain object. A JsMap with any other key
//   type is an array of [key, value] pairs, in insertion order (JSON.stringify
//   writes {} for a JS Map, so the TypeScript side converts those to pairs).
// - DateOnly is "yyyy-MM-dd" (System.Text.Json's own format).
// - Strings are escaped as JSON.stringify escapes them - quotes, backslashes
//   and control characters only - so "it's 07:00–09:00" is written as is,
//   not as "it\u0027s 07:00\u201309:00" (the default encoder's HTML-safe form).

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Roster.Engine.Core;

public static class RosterJson
{
    /// <summary>The options every engine (de)serialisation uses.</summary>
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        o.Converters.Add(new JsMapJsonConverterFactory());
        o.Converters.Add(new JsSetJsonConverterFactory());
        return o;
    }
}

public sealed class JsMapJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(JsMap<,>);

    public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options)
    {
        var args = t.GetGenericArguments();
        var conv = args[0] == typeof(string)
            ? typeof(StringKeyedMapConverter<>).MakeGenericType(args[1])
            : typeof(PairMapConverter<,>).MakeGenericType(args[0], args[1]);
        return (JsonConverter)Activator.CreateInstance(conv)!;
    }

    private sealed class StringKeyedMapConverter<TValue> : JsonConverter<JsMap<string, TValue>>
    {
        public override JsMap<string, TValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("expected an object for a string-keyed JsMap");
            var map = new JsMap<string, TValue>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) return map;
                var key = reader.GetString()!;
                reader.Read();
                map.Set(key, JsonSerializer.Deserialize<TValue>(ref reader, options)!);
            }
            throw new JsonException("unterminated object");
        }

        public override void Write(Utf8JsonWriter writer, JsMap<string, TValue> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var (k, v) in value)
            {
                writer.WritePropertyName(k);
                JsonSerializer.Serialize(writer, v, options);
            }
            writer.WriteEndObject();
        }
    }

    private sealed class PairMapConverter<TKey, TValue> : JsonConverter<JsMap<TKey, TValue>> where TKey : notnull
    {
        public override JsMap<TKey, TValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("expected an array of [key, value] pairs for a JsMap");
            var map = new JsMap<TKey, TValue>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return map;
                if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("expected a [key, value] pair");
                reader.Read();
                var key = JsonSerializer.Deserialize<TKey>(ref reader, options)!;
                reader.Read();
                var val = JsonSerializer.Deserialize<TValue>(ref reader, options)!;
                reader.Read();
                if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("a pair has exactly two entries");
                map.Set(key, val);
            }
            throw new JsonException("unterminated array");
        }

        public override void Write(Utf8JsonWriter writer, JsMap<TKey, TValue> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var (k, v) in value)
            {
                writer.WriteStartArray();
                JsonSerializer.Serialize(writer, k, options);
                JsonSerializer.Serialize(writer, v, options);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
    }
}

public sealed class JsSetJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(JsSet<>);

    public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(SetConverter<>).MakeGenericType(t.GetGenericArguments()[0]))!;

    private sealed class SetConverter<T> : JsonConverter<JsSet<T>> where T : notnull
    {
        public override JsSet<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(JsonSerializer.Deserialize<List<T>>(ref reader, options) ?? []);

        public override void Write(Utf8JsonWriter writer, JsSet<T> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToList(), options);
    }
}
