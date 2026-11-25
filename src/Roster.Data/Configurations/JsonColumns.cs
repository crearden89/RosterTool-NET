// New: JSON (de)serialisation for the nvarchar(max) JSON columns (Postgres jsonb).
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Roster.Data.Configurations;

internal static class JsonColumns
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
}
