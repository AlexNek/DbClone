using System.Text.Json;
using System.Text.Json.Serialization;

namespace DatabaseInterchange.Format;

/// <summary>
/// JSON serialization options for the catalog format.
/// </summary>
internal static class CatalogJson
{
    /// <summary>
    /// Gets the JSON serializer options for writing catalog files.
    /// </summary>
    public static JsonSerializerOptions WriterOptions { get; } = CreateWriterOptions();

    /// <summary>
    /// Gets the JSON serializer options for reading catalog files.
    /// </summary>
    public static JsonSerializerOptions ReaderOptions { get; } = CreateReaderOptions();

    /// <summary>
    /// Gets the JSON serializer options for the <c>.ndjson</c> row files.
    /// </summary>
    /// <remarks>
    /// A chunk holds one JSON array per line and the reader takes a line as a row, so a row is written
    /// without indentation: the document <see cref="WriterOptions"/> produces spans several lines, and
    /// the first of them is an array that never closes.
    /// </remarks>
    public static JsonSerializerOptions RowWriterOptions { get; } = new();

    private static JsonSerializerOptions CreateWriterOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static JsonSerializerOptions CreateReaderOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
