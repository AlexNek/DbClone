using System.Globalization;
using System.Text.Json;

namespace DbClone.UI.Services.Relationship;

/// <summary>
/// Where the relationship picture stands: the scale the reader zoomed to and the shift they panned by.
/// The viewer states it in the address it serves the document at, so a picture drawn again for another
/// cluster selection opens where the one it replaces stood — switching a chip changes what is drawn,
/// never where the reader is looking (T28).
/// </summary>
/// <param name="Scale">The zoom the picture is drawn at, in the document's own units.</param>
/// <param name="X">The horizontal shift the picture was panned by, in pixels of the stage.</param>
/// <param name="Y">The vertical shift the picture was panned by, in pixels of the stage.</param>
internal readonly record struct GraphCamera(double Scale, double X, double Y)
{
    /// <summary>How long the document's statement may be: three numbers never need more.</summary>
    private const int MaxLength = 256;

    /// <summary>
    /// Reads the camera the document states about itself, which its script answers as
    /// <c>{"s":1,"x":0,"y":0}</c>. A statement of any other shape is refused whole rather than guessed
    /// at, and a document that states none — one opened outside the viewer — is taken at its own size.
    /// </summary>
    /// <param name="json">The value the document's script answered with.</param>
    /// <param name="camera">The camera it states.</param>
    /// <returns>Whether the document stated a camera.</returns>
    internal static bool TryRead(string? json, out GraphCamera camera)
    {
        camera = default;

        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxLength)
        {
            return false;
        }

        try
        {
            using var statement = JsonDocument.Parse(json);
            var payload = statement.RootElement;

            if (payload.ValueKind != JsonValueKind.Object
                || !TryReadNumber(payload, "s", out var scale)
                || !TryReadNumber(payload, "x", out var x)
                || !TryReadNumber(payload, "y", out var y))
            {
                return false;
            }

            camera = new GraphCamera(scale, x, y);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// States the camera as the query the document is served at, which its script reads back before a
    /// picture is placed. The numbers are written for the script, never for the machine's own culture.
    /// </summary>
    /// <returns>The parameters, each led by <c>&amp;</c> so they extend the address's own version.</returns>
    internal string Query() => $"&s={Num(Scale)}&x={Num(X)}&y={Num(Y)}";

    private static bool TryReadNumber(JsonElement payload, string name, out double value)
    {
        value = 0;

        return payload.TryGetProperty(name, out var entry)
            && entry.ValueKind == JsonValueKind.Number
            && entry.TryGetDouble(out value)
            && double.IsFinite(value);
    }

    private static string Num(double value) => value.ToString(CultureInfo.InvariantCulture);
}
