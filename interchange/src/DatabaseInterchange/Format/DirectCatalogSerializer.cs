using System.Text.Json;

using CanonicalModel.Models;

namespace DatabaseInterchange.Format;

/// <summary>
/// Serializes <see cref="DatabaseModel"/> directly to catalog format for same-type transfers.
/// No conversion to interchange types — maximum fidelity, minimum overhead.
/// </summary>
public sealed class DirectCatalogSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Serializes a <see cref="DatabaseModel"/> to JSON for catalog storage.
    /// </summary>
    public string Serialize(DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return JsonSerializer.Serialize(model, SerializerOptions);
    }

    /// <summary>
    /// Deserializes a <see cref="DatabaseModel"/> from catalog JSON.
    /// </summary>
    public DatabaseModel? Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<DatabaseModel>(json, SerializerOptions);
    }
}
