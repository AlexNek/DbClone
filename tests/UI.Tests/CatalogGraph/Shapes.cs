namespace UI.Tests.CatalogGraph;

/// <summary>
/// Catalogs nobody wrote down: one shape per seed, drawn the way a small schema grows — every table reaches
/// an earlier one, and a few relations then cross the picture. Determinism comes from the seed, so a failing
/// shape can be named by its number alone.
/// </summary>
internal static class Shapes
{
    /// <summary>How many tables a generated shape has: enough for several rings and a settlement.</summary>
    public const int Nodes = 24;

    /// <summary>The relations of one generated shape.</summary>
    /// <param name="seed">Which shape.</param>
    /// <returns>The relations as (child, parent) pairs.</returns>
    public static IReadOnlyList<(int Child, int Parent)> Edges(int seed)
    {
        var random = new Random(seed);
        var edges = new List<(int Child, int Parent)>();

        for (var node = 1; node < Nodes; node++)
        {
            edges.Add((node, random.Next(node)));
        }

        for (var extra = 0; extra < Nodes / 2; extra++)
        {
            edges.Add((random.Next(Nodes), random.Next(Nodes)));
        }

        return edges;
    }
}
