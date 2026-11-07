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
