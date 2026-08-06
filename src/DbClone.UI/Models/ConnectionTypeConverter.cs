using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbClone.UI.Models;

/// <summary>
/// Deserializes the ConnectionType field as a lowercase stable platform id.
/// Returns null for unrecognized legacy formats (integers, nulls) — the caller
/// resolves null to the base engine default via PlatformSchemaResolver.
/// No engine or platform knowledge in this converter.
/// </summary>
public sealed class ConnectionTypeConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString()?.ToLowerInvariant();

        // Legacy integers, nulls, or unexpected tokens → unrecognized
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}
