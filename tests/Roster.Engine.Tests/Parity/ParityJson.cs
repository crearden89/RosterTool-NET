// New: how the parity harness reads the corpus and writes the C# engine's
// results - RosterJson's conventions (camelCase TypeScript names, JsMap as an
// object or [key, value] pairs, JsSet as an array) plus the corpus's own two:
// NaN and the infinities as the strings "NaN", "Infinity" and "-Infinity", and
// an empty Map written as [] whatever its key type (the dumper cannot tell a
// Map's key type when it has no keys).

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Roster.Engine.Core;

namespace Roster.Engine.Tests.Parity;

public static class ParityJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = RosterJson.Create();
        o.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
        o.Converters.Insert(0, new EmptyMapTolerantFactory());
        return o;
    }

    public static T Read<T>(JsonNode? node) => node.Deserialize<T>(Options)!;

    /// <summary>A C# value as the corpus would write it (re-parsed, so every number is a plain JSON number).</summary>
    public static JsonNode? ToNode(object? value) =>
        value is null ? null : JsonNode.Parse(JsonSerializer.Serialize(value, value.GetType(), Options));

    // A JsMap reads [] or {} as empty, whatever its key type; anything else is
    // RosterJson's own converter's business.
    private sealed class EmptyMapTolerantFactory : JsonConverterFactory
    {
        private readonly JsMapJsonConverterFactory _inner = new();

        public override bool CanConvert(Type t) => _inner.CanConvert(t);

        public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(
                typeof(Tolerant<,>).MakeGenericType(t.GetGenericArguments()),
                _inner.CreateConverter(t, options))!;

        private sealed class Tolerant<TKey, TValue>(JsonConverter inner) : JsonConverter<JsMap<TKey, TValue>> where TKey : notnull
        {
            private readonly JsonConverter<JsMap<TKey, TValue>> _inner = (JsonConverter<JsMap<TKey, TValue>>)inner;

            public override JsMap<TKey, TValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                {
                    var peek = reader;
                    peek.Read();
                    if (peek.TokenType is JsonTokenType.EndArray or JsonTokenType.EndObject)
                    {
                        reader.Read();
                        return new JsMap<TKey, TValue>();
                    }
                }
                return _inner.Read(ref reader, typeToConvert, options)!;
            }

            public override void Write(Utf8JsonWriter writer, JsMap<TKey, TValue> value, JsonSerializerOptions options) =>
                _inner.Write(writer, value, options);
        }
    }
}
