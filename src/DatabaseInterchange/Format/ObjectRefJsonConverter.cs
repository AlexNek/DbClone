using System.Text.Json;
using System.Text.Json.Serialization;

using CanonicalModel.Interchange;

namespace DatabaseInterchange.Format;

/// <summary>
/// Serializes an <see cref="ObjectRef"/> as a plain string (its <see cref="ObjectRef.Value"/>)
/// rather than as a nested object, so the catalog JSON stays compact and human-readable.
/// </summary>
internal sealed class ObjectRefJsonConverter : JsonConverter<ObjectRef>
{
    public override ObjectRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.Null)
        {
            return default;
        }

        if (reader.TokenType is not JsonTokenType.String)
        {
            throw new JsonException($"Expected a string token for ObjectRef, got {reader.TokenType}.");
        }

        return new ObjectRef(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, ObjectRef value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}
