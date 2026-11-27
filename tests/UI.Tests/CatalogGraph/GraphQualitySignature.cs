using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DbClone.UI.Services.GraphLayout;

namespace UI.Tests.CatalogGraph;

/// <summary>A complete invariant fingerprint: canvas, cards, route meaning, geometry, controls and labels.</summary>
internal static class GraphQualitySignature
{
    public static string Of(IReadOnlyList<NodePlacement> nodes, IReadOnlyList<EdgeRoute> routes, double width, double height)
    {
        var text = new StringBuilder();
        Add(text, width, height);
        foreach (var node in nodes.OrderBy(node => node.NodeIndex))
        {
            text.Append(node.NodeIndex.ToString(CultureInfo.InvariantCulture)).Append(':');
            Add(text, node.TopLeft.X, node.TopLeft.Y, node.Width, node.Height);
        }

        foreach (var route in routes.OrderBy(route => route.EdgeIndex))
        {
            text.Append(route.EdgeIndex.ToString(CultureInfo.InvariantCulture)).Append(':').Append(route.Shape).Append(':').Append(route.Geometry).Append(':');
            foreach (var point in route.Points)
            {
                Add(text, point.X, point.Y);
            }

            Add(text, route.LabelPoint.X, route.LabelPoint.Y);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void Add(StringBuilder text, params double[] values)
    {
        foreach (var value in values)
        {
            text.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        }

        text.Append('|');
    }
}
