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
    /// Serializes a <see cref="DatabaseModel"/> straight into a catalog stream.
    /// </summary>
    /// <remarks>
    /// The document is emitted into the stream's own buffer rather than built as a <see cref="string"/> and
    /// copied over, which is the rule part A/R4 states for every stored catalog file.
    /// </remarks>
    /// <param name="stream">The destination stream.</param>
    /// <param name="model">The model to write.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SerializeAsync(Stream stream, DatabaseModel model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(model);
        await JsonSerializer.SerializeAsync(stream, model, SerializerOptions, ct);
    }

    /// <summary>
    /// Deserializes a <see cref="DatabaseModel"/> from a catalog stream.
    /// </summary>
    /// <param name="stream">The source stream, positioned after any byte-order mark.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The model, or <see langword="null"/> when the stream holds no value.</returns>
    public async Task<DatabaseModel?> DeserializeAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return await JsonSerializer.DeserializeAsync<DatabaseModel>(stream, SerializerOptions, ct);
    }
}
